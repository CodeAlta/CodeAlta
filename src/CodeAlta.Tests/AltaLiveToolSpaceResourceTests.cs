using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public async Task AgentPrompts_OfAnotherSpace_AreNotListed_NorRun_AndANewSessionStartsWithTheAgentOfItsSpace()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync().ConfigureAwait(false);
        fixture.WriteConfig("""
            [prompts.spaces]
            plan = ["business"]
            """);
        await fixture.WritePromptAsync("reviewer").ConfigureAwait(false);
        Assert.AreEqual(SpaceChangeStatus.Ok, (await fixture.Spaces.UpdateSettingsAsync("business", settings => settings with { NewSessions = new SpaceNewSessions("plan") }).ConfigureAwait(false)).Status);
        var business = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business").ConfigureAwait(false);
        var personal = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);

        // A new session takes the agent of its space, unless one is asked for.
        Assert.AreEqual("plan", business.GetProperty("modelSelection").GetProperty("promptId").GetString());
        Assert.AreEqual("default", personal.GetProperty("modelSelection").GetProperty("promptId").GetString());
        var asked = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business", "--prompt-id", "reviewer").ConfigureAwait(false);
        Assert.AreEqual("reviewer", asked.GetProperty("modelSelection").GetProperty("promptId").GetString());

        var personalAgent = Agent(personal);
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "create", "--global", "--prompt-id", "plan"], caller: personalAgent).ConfigureAwait(false), "prompt.notInSpace");
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "set_agent", Id(personal), "--prompt-id", "plan"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false), "prompt.notInSpace");
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "send", Id(personal), "--message", "hello", "--prompt-id", "plan"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false), "prompt.notInSpace");
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["prompt", "show", "plan"], caller: personalAgent).ConfigureAwait(false), "prompt.notInSpace");
        var setInBusiness = await fixture.Dispatcher.InvokeAsync(["session", "set_agent", Id(business), "--prompt-id", "plan"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, setInBusiness.ExitCode, setInBusiness.Stdout + setInBusiness.Stderr);

        // A session lists the prompts of its space; the user lists them all.
        CollectionAssert.DoesNotContain(await fixture.PromptIdsAsync(personalAgent).ConfigureAwait(false), "plan");
        CollectionAssert.Contains(await fixture.PromptIdsAsync(personalAgent).ConfigureAwait(false), "reviewer", "A prompt that lists no space is in every space.");
        CollectionAssert.Contains(await fixture.PromptIdsAsync(Agent(business)).ConfigureAwait(false), "plan");
        CollectionAssert.Contains(await fixture.PromptIdsAsync(AltaCallerIdentity.Cli).ConfigureAwait(false), "plan");
    }

    [TestMethod]
    public async Task ASubSession_RunsTheDefaultAgent_EvenUnderAPlanSessionOfASpaceThatStartsInPlan_UnlessAnotherIsAskedFor()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync().ConfigureAwait(false);
        Assert.AreEqual(SpaceChangeStatus.Ok, (await fixture.Spaces.UpdateSettingsAsync("business", settings => settings with { NewSessions = new SpaceNewSessions("plan") }).ConfigureAwait(false)).Status);
        var planner = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business").ConfigureAwait(false);
        Assert.AreEqual("plan", planner.GetProperty("modelSelection").GetProperty("promptId").GetString());

        var child = await fixture.CreateAsync(Agent(planner), "--global").ConfigureAwait(false);
        Assert.AreEqual(Id(planner), child.GetProperty("parentSessionId").GetString());
        Assert.AreEqual("default", child.GetProperty("modelSelection").GetProperty("promptId").GetString());
        var asked = await fixture.CreateAsync(Agent(planner), "--global", "--prompt-id", "plan").ConfigureAwait(false);
        Assert.AreEqual("plan", asked.GetProperty("modelSelection").GetProperty("promptId").GetString());
        var detached = await fixture.CreateAsync(Agent(planner), "--global", "--no-parent").ConfigureAwait(false);
        Assert.AreEqual("plan", detached.GetProperty("modelSelection").GetProperty("promptId").GetString(), "A session without a parent starts with the agent of its space.");
    }

    [TestMethod]
    public async Task ANewSession_StartsWithAProviderOfItsSpace_AndOneAskedForOutsideItIsRefused()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync("claude-business").ConfigureAwait(false);
        fixture.WriteConfig("""
            [chat]
            default_provider = "claude-business"

            [providers.claude-business]
            type = "claude-code"
            spaces = ["business"]
            """);
        static string? Provider(JsonElement created) => created.GetProperty("modelSelection").GetProperty("providerKey").GetString();

        // The default provider where it is; the first provider of the space where it is not.
        Assert.AreEqual("claude-business", Provider(await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business").ConfigureAwait(false)));
        var personal = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        Assert.AreEqual("alpha", Provider(personal));

        // A provider asked for is never replaced: one of another space is refused, for the user and for an agent.
        var refused = await fixture.Dispatcher.InvokeAsync(["session", "create", "--global", "--space", "personal", "--provider", "claude-business"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        fixture.AssertRefused(refused, "provider.notInSpace");
        StringAssert.Contains(refused.Stdout + refused.Stderr, "space 'personal'");
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "create", "--global", "--model-ref", "claude-business:sonnet"], caller: Agent(personal)).ConfigureAwait(false),
            "provider.notInSpace");
        Assert.AreEqual("alpha", Provider(await fixture.CreateAsync(Agent(personal), "--global").ConfigureAwait(false)), "An agent creates in its own space, with a provider of it.");
    }

    [TestMethod]
    public async Task Skills_OfAnotherSpace_AreNotListed_NorActivated()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync().ConfigureAwait(false);
        fixture.WriteConfig("""
            [skills.spaces]
            terraform = ["business", "default"]
            """);
        await WriteSkillAsync(Path.Combine(fixture.Options.GlobalRoot!, "skills", "terraform"), "terraform", "Terraform plans.").ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(fixture.Options.GlobalRoot!, "skills", "notes"), "notes", "Notes.").ConfigureAwait(false);
        var personal = Agent(await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false));
        var business = Agent(await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business").ConfigureAwait(false));

        CollectionAssert.DoesNotContain(await fixture.SkillNamesAsync(personal).ConfigureAwait(false), "terraform");
        CollectionAssert.Contains(await fixture.SkillNamesAsync(personal).ConfigureAwait(false), "notes");
        CollectionAssert.Contains(await fixture.SkillNamesAsync(business).ConfigureAwait(false), "terraform");
        CollectionAssert.Contains(await fixture.SkillNamesAsync(AltaCallerIdentity.Cli).ConfigureAwait(false), "terraform");

        var refused = await fixture.Dispatcher.InvokeAsync(["skill", "activate", "terraform"], caller: personal).ConfigureAwait(false);
        fixture.AssertRefused(refused, "skill.notInSpace");
        StringAssert.Contains(refused.Stdout + refused.Stderr, "space 'personal'");
    }

    [TestMethod]
    public async Task TheSettingsOfAPluginOfAProject_AreReachedOnlyFromTheSpacesOfItsProject()
    {
        var settings = new ProjectPluginSettings();
        await using var fixture = await SpaceResourceFixture.CreateAsync(withOwner: false, withLibrary: false, settings).ConfigureAwait(false);
        var folder = Path.Combine(Path.GetDirectoryName(fixture.Options.GlobalRoot!)!, "work");
        Directory.CreateDirectory(folder);
        var project = await fixture.Spaces.Projects.UpsertFromPathAsync(folder).ConfigureAwait(false);
        await fixture.Spaces.AssignAsync(project.Id, ["business"], null).ConfigureAwait(false);
        settings.Project = project.Id;
        var personal = Agent(await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false));
        var business = Agent(await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business").ConfigureAwait(false));

        var listed = await fixture.Dispatcher.InvokeAsync(["settings", "list"], caller: personal).ConfigureAwait(false);
        Assert.IsFalse(listed.Stdout.Contains("plugins.builds.poll", StringComparison.Ordinal), listed.Stdout);
        var refused = await fixture.Dispatcher.InvokeAsync(["settings", "set", "plugins.builds.poll", "9"], caller: personal).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.NotFound, refused.ExitCode, refused.Stdout + refused.Stderr);
        Assert.AreEqual(0, settings.Writes.Count, "a session of another space does not reach the project");

        var changed = await fixture.Dispatcher.InvokeAsync(["settings", "set", "plugins.builds.poll", "9"], caller: business).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, changed.ExitCode, changed.Stdout + changed.Stderr);
        Assert.AreEqual(project.Id, settings.Writes.Single(), "changed for the project of the plugin");
    }

    [TestMethod]
    public async Task AnAgent_ForksAndSendsTo_OnlyTheSessionsOfItsOwnSpace()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync().ConfigureAwait(false);
        var business = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business").ConfigureAwait(false);
        var personal = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        var personalAgent = Agent(personal);

        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "fork", Id(business)], caller: personalAgent).ConfigureAwait(false), "space.notOwnSpace");
        foreach (var verb in new[] { "send", "queue", "message" })
        {
            fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", verb, Id(business), "--message", "hello"], caller: personalAgent).ConfigureAwait(false), "space.notOwnSpace");
        }

        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["reminder", "create", "--session", Id(business), "--duration", "00:10:00", "--content", "check"], caller: personalAgent).ConfigureAwait(false), "space.notOwnSpace");

        // The user reaches every session.
        var queued = await fixture.Dispatcher.InvokeAsync(["session", "queue", Id(business), "--message", "hello"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.IsFalse((queued.Stdout + queued.Stderr).Contains("space.notOwnSpace", StringComparison.Ordinal), queued.Stdout + queued.Stderr);
    }

    [TestMethod]
    public async Task AnAgent_StillReachesItsOwnDescendants_AfterItsProjectJoinsAnotherSpace()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync().ConfigureAwait(false);
        var folder = Path.Combine(Path.GetDirectoryName(fixture.Options.GlobalRoot!)!, "work");
        Directory.CreateDirectory(folder);
        var project = await fixture.Spaces.Projects.UpsertFromPathAsync(folder).ConfigureAwait(false);

        // A session of 1.8.0 records no space: its project is in none, so it is in the default space, where its children are created.
        fixture.View.ShownSpaceId = null;
        var parent = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--project", project.Id).ConfigureAwait(false);
        Assert.IsNull((await fixture.Runtime.GetSessionSpaceAsync(Id(parent)).ConfigureAwait(false))!.SpaceId);
        var parentAgent = Agent(parent);
        var child = await fixture.CreateAsync(parentAgent, "--project", project.Id).ConfigureAwait(false);
        var grandchild = await fixture.CreateAsync(Agent(child), "--global").ConfigureAwait(false);
        var unrelated = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "default").ConfigureAwait(false);
        Assert.AreEqual(SpaceDescriptor.DefaultId, (await fixture.Runtime.GetSessionSpaceAsync(Id(child)).ConfigureAwait(false))!.SpaceId);

        // Its project joins a space: the parent follows it, and its descendants stay in the space they were created in.
        Assert.IsTrue((await fixture.Spaces.AssignAsync(project.Id, ["business"], null).ConfigureAwait(false)).Succeeded);
        void AssertAllowed(AltaCommandResult result)
            => Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);

        AssertAllowed(await fixture.Dispatcher.InvokeAsync(["session", "queue", Id(child), "--message", "hello"], caller: parentAgent).ConfigureAwait(false));
        AssertAllowed(await fixture.Dispatcher.InvokeAsync(["reminder", "create", "--session", Id(grandchild), "--duration", "00:10:00", "--content", "check"], caller: parentAgent).ConfigureAwait(false));
        AssertAllowed(await fixture.Dispatcher.InvokeAsync(["session", "fork", Id(child)], caller: parentAgent).ConfigureAwait(false));
        AssertAllowed(await fixture.Dispatcher.InvokeAsync(["session", "archive", Id(grandchild)], caller: parentAgent).ConfigureAwait(false));

        // A session of the old space that it did not create stays out of its reach, and a child does not reach its parent.
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "queue", Id(unrelated), "--message", "hello"], caller: parentAgent).ConfigureAwait(false), "space.notOwnSpace");
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "archive", Id(unrelated)], caller: parentAgent).ConfigureAwait(false), "space.notOwnSpace");
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "queue", Id(parent), "--message", "hello"], caller: Agent(child)).ConfigureAwait(false), "space.notOwnSpace");
    }

    [TestMethod]
    public async Task ASandboxedAgent_RunsOnlyTheCommandsThatKeepItInItsSandbox()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync().ConfigureAwait(false);
        fixture.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        var agent = Agent(await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false));

        // Each of them runs here, outside the sandbox, and changes what runs outside it.
        foreach (var arguments in new[] { new[] { "prompt", "create", "mine", "--scope", "global", "--stdin" }, ["space", "create", "--name", "Mine"], ["space", "update", "personal", "--name", "Mine"], ["project", "add", fixture.Options.GlobalRoot!] })
        {
            var refused = await fixture.Dispatcher.InvokeAsync(arguments, caller: agent).ConfigureAwait(false);
            AssertSandboxRefused(refused, "sandbox.refused");
            StringAssert.Contains(refused.Stdout + refused.Stderr, "profile 'workspace'");
        }

        Assert.AreEqual(AltaExitCodes.Success, (await fixture.Dispatcher.InvokeAsync(["space", "list"], caller: agent).ConfigureAwait(false)).ExitCode, "reading is allowed");
        Assert.AreEqual(AltaExitCodes.Success, (await fixture.Dispatcher.InvokeAsync(["prompt", "create", "--help"], caller: agent).ConfigureAwait(false)).ExitCode, "and the help of every command");
        var user = await fixture.Dispatcher.InvokeAsync(["space", "create", "--name", "Mine"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, user.ExitCode, "the user is not sandboxed: " + user.Stdout + user.Stderr);
    }

    [TestMethod]
    public async Task ASandboxedAgent_ReachesOnlyItselfAndTheSessionsItCreated()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync().ConfigureAwait(false);
        fixture.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        var creator = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        var agent = Agent(creator);
        var child = await fixture.CreateAsync(agent, "--global").ConfigureAwait(false);
        var unrelated = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);

        void AssertAllowed(AltaCommandResult result) => Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        AssertAllowed(await fixture.Dispatcher.InvokeAsync(["session", "queue", Id(child), "--message", "hello"], caller: agent).ConfigureAwait(false));
        AssertAllowed(await fixture.Dispatcher.InvokeAsync(["session", "queue", Id(creator), "--message", "later"], caller: agent).ConfigureAwait(false));
        AssertAllowed(await fixture.Dispatcher.InvokeAsync(["session", "fork", Id(child)], caller: agent).ConfigureAwait(false));
        foreach (var arguments in new[]
        {
            new[] { "session", "queue", Id(unrelated), "--message", "hello" }, ["session", "send", Id(unrelated), "--message", "hello"], ["session", "message", Id(unrelated), "--message", "hello"],
            ["session", "fork", Id(unrelated)], ["session", "create", "--global", "--parent", Id(unrelated)],
            ["reminder", "create", "--session", Id(unrelated), "--duration", "00:10:00", "--content", "check"],
        })
        {
            AssertSandboxRefused(await fixture.Dispatcher.InvokeAsync(arguments, caller: agent).ConfigureAwait(false), "sandbox.notOwnSession");
        }

        // A session that is not sandboxed reaches the others of its space, as before.
        fixture.WriteConfig("");
        AssertAllowed(await fixture.Dispatcher.InvokeAsync(["session", "queue", Id(unrelated), "--message", "hello"], caller: agent).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ASandboxedAgent_ReachesNeitherAnotherProject_NorASessionOutOfItsSandbox_NorWhatOtherSessionsRun()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync(withOwner: true).ConfigureAwait(false);
        fixture.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        var agent = Agent(await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false));
        var unrelated = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        Task<AltaCommandResult> Run(AltaCallerIdentity caller, params string[] arguments) => fixture.Dispatcher.InvokeAsync(arguments, "{}", caller).AsTask();

        // A session works in the folder of its project, which its sandbox writes: not in another one.
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(fixture.Options.GlobalRoot!)!, "elsewhere")).FullName;
        var project = await fixture.Spaces.Projects.UpsertFromPathAsync(folder).ConfigureAwait(false);
        AssertSandboxRefused(await Run(agent, "session", "create", "--project", project.Id).ConfigureAwait(false), "sandbox.projectDenied");

        // A session it created, whose sandbox the user turned off since, is out of its reach.
        var child = await fixture.CreateAsync(agent, "--global").ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, (await Run(agent, "session", "queue", Id(child), "--message", "hello").ConfigureAwait(false)).ExitCode, "in its sandbox, it is reached");
        var turnedOff = await Run(AltaCallerIdentity.Cli, "session", "queue", Id(child), "--message", "hi", "--sandbox", "off").ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, turnedOff.ExitCode, turnedOff.Stdout + turnedOff.Stderr);
        AssertSandboxRefused(await Run(agent, "session", "queue", Id(child), "--message", "hello").ConfigureAwait(false), "sandbox.notOwnSession");

        // Nor does it ask for another session, nor list the jobs of every one.
        AssertSandboxRefused(await Run(agent, "ask", "--session", Id(unrelated), "--stdin").ConfigureAwait(false), "sandbox.notOwnSession");
        AssertSandboxRefused(await Run(agent, "job", "list", "--all").ConfigureAwait(false), "sandbox.notOwnSession");
        AssertSandboxRefused(await Run(agent, "job", "list", "--session", Id(unrelated)).ConfigureAwait(false), "sandbox.notOwnSession");
    }

    [TestMethod]
    public async Task SessionCommands_ChooseTheSandbox_AndAnAgentTurnsOneOnButNeverOff()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync(withOwner: true).ConfigureAwait(false);
        static JsonElement Sandbox(JsonElement record) => record.GetProperty("sandbox");

        // The user chooses any; the record says which, and where it comes from.
        var strict = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal", "--sandbox", "strict").ConfigureAwait(false);
        Assert.AreEqual(("strict", "session"), (Sandbox(strict).GetProperty("profile").GetString(), Sandbox(strict).GetProperty("source").GetString()));
        // Off as its own choice: no setting sandboxes it.
        var plain = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal", "--sandbox", "off").ConfigureAwait(false);
        Assert.IsFalse(Sandbox(plain).TryGetProperty("profile", out _), "a session that is not sandboxed has no profile");
        Assert.AreEqual("session", Sandbox(plain).GetProperty("source").GetString());
        AssertSandboxRefused(await fixture.Dispatcher.InvokeAsync(["session", "create", "--global", "--sandbox", "nowhere"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false), "usage.invalidSandbox", AltaExitCodes.Usage);

        // An agent turns a sandbox on for a session that has none, and never takes one away nor replaces it.
        var agent = Agent(plain);
        var boxed = await fixture.CreateAsync(agent, "--global", "--sandbox", "workspace").ConfigureAwait(false);
        Assert.AreEqual("workspace", Sandbox(boxed).GetProperty("profile").GetString());
        AssertSandboxRefused(await fixture.Dispatcher.InvokeAsync(["session", "create", "--global", "--sandbox", "off"], caller: agent).ConfigureAwait(false), "sandbox.choiceDenied");
        var child = await fixture.CreateAsync(agent, "--global").ConfigureAwait(false);
        var turnedOn = await fixture.Dispatcher.InvokeAsync(["session", "queue", Id(child), "--message", "hi", "--sandbox", "strict"], caller: agent).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, turnedOn.ExitCode, turnedOn.Stdout + turnedOn.Stderr);
        Assert.AreEqual("strict", (await fixture.Runtime.GetSessionSandboxAsync(Id(child)).ConfigureAwait(false)).Choice.Profile);
        AssertSandboxRefused(await fixture.Dispatcher.InvokeAsync(["session", "queue", Id(child), "--message", "hi", "--sandbox", "workspace"], caller: agent).ConfigureAwait(false), "sandbox.choiceDenied");
        AssertSandboxRefused(await fixture.Dispatcher.InvokeAsync(["session", "queue", Id(child), "--message", "hi", "--sandbox", "scope"], caller: agent).ConfigureAwait(false), "sandbox.choiceDenied");

        // A session the settings sandbox keeps the sandbox of the user when an agent asks for another.
        fixture.WriteConfig("[sandbox]\nall_sessions = \"strict\"\n");
        var kept = await fixture.CreateAsync(agent, "--global", "--sandbox", "workspace").ConfigureAwait(false);
        Assert.AreEqual(("strict", "all_sessions"), (Sandbox(kept).GetProperty("profile").GetString(), Sandbox(kept).GetProperty("source").GetString()));

        // A sandboxed agent chooses no sandbox: what it creates has its own.
        AssertSandboxRefused(await fixture.Dispatcher.InvokeAsync(["session", "create", "--global", "--sandbox", "strict"], caller: Agent(strict)).ConfigureAwait(false), "sandbox.choiceDenied");
    }

    [TestMethod]
    public async Task AScriptOfASandboxedSession_IsRefusedWhatItsSandboxForbids_AndReadsTheLibraryWithoutChangingIt()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync(withOwner: true, withLibrary: true).ConfigureAwait(false);
        var strict = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal", "--sandbox", "strict").ConfigureAwait(false);
        var unrelated = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        var library = new CodeAlta.Orchestration.Runtime.Scripts.CodeModeScriptLibrary(fixture.Options);
        Assert.IsNull(library.Save("count", CodeAlta.Orchestration.Runtime.Scripts.CodeModeScriptScope.Global, "return 1;", null, null).Refusal);
        // The identity of a script of the session: the session, and its run.
        var identity = Agent(strict) with { ScriptRunId = "run-sandboxed" };
        var binding = new CodeAlta.LiveTool.Scripts.AltaScriptBinding(fixture.Dispatcher.Describe(identity, null));

        var result = await new CodeAlta.CodeMode.CodeModeHost(new CodeAlta.CodeMode.CodeModeApi(binding.Functions), new CodeAlta.CodeMode.CodeModeScriptCache())
            .RunAsync(CodeAlta.CodeMode.CodeModeSource.Prepare($$"""
                const outcomes = {};
                const attempt = async (name, call) => {
                  try { await call(); outcomes[name] = "ok"; } catch (e) { outcomes[name] = `${e.name}:${e.code}`; }
                };
                await attempt("createUnsandboxed", () => alta.session.create({ global: true, sandbox: "off" }));
                await attempt("queueOther", () => alta.session.queue({ sessionId: "{{Id(unrelated)}}", message: "hi" }));
                await attempt("jobsOfAll", () => alta.job.list({ all: true }));
                await attempt("save", () => alta.script.save({ name: "mine", stdin: "return 2;" }));
                await attempt("delete", () => alta.script.delete({ name: "count" }));
                await attempt("list", () => alta.script.list({}));
                await attempt("show", () => alta.script.show({ name: "count" }));
                await attempt("check", () => alta.script.check({ stdin: "return 3;" }));
                await attempt("edit", () => alta.script.edit({ name: "count" }));
                return outcomes;
                """), new CodeAlta.LiveTool.Scripts.AltaScriptCallHandler(fixture.Dispatcher, binding, identity, null)).ConfigureAwait(false);

        Assert.AreEqual(CodeAlta.CodeMode.CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        using var outcomes = JsonDocument.Parse(result.ValueJson!);
        string Outcome(string name) => outcomes.RootElement.GetProperty(name).GetString()!;
        Assert.AreEqual("AltaError:sandbox.choiceDenied", Outcome("createUnsandboxed"), "a script asks no session out of its sandbox");
        Assert.AreEqual("AltaError:sandbox.notOwnSession", Outcome("queueOther"));
        Assert.AreEqual("AltaError:sandbox.notOwnSession", Outcome("jobsOfAll"));
        StringAssert.StartsWith(Outcome("save"), "AltaError:sandbox.", "a sandboxed script saves no script in the library");
        StringAssert.StartsWith(Outcome("delete"), "AltaError:sandbox.", "nor deletes one");
        foreach (var allowed in new[] { "list", "show", "check", "edit" })
        {
            Assert.AreEqual("ok", Outcome(allowed), $"a sandboxed script still runs alta.script.{allowed}");
        }

        Assert.IsNull(library.Find("mine", null), "nothing was saved");
        Assert.IsNotNull(library.Find("count", null), "nothing was deleted");
    }

    [TestMethod]
    public async Task ScriptsOfAnotherSpace_AreNotListedNorShownNorRun_AndScriptRunStartsASessionOfTheProjectThatRunsIt()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync(withOwner: true, withLibrary: true).ConfigureAwait(false);
        fixture.WriteConfig("""
            [scripts.spaces]
            review = ["business"]
            """);
        var folder = Path.Combine(Path.GetDirectoryName(fixture.Options.GlobalRoot!)!, "work");
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        var project = await fixture.Spaces.Projects.UpsertFromPathAsync(folder).ConfigureAwait(false);
        var library = new CodeAlta.Orchestration.Runtime.Scripts.CodeModeScriptLibrary(fixture.Options);
        Assert.IsNull(library.Save("review", CodeAlta.Orchestration.Runtime.Scripts.CodeModeScriptScope.Global,
            "export const meta = { name: \"review\", description: \"Reviews.\", params: { pr: { type: \"integer\" } } };\nreturn args.pr;", null, null).Refusal);
        Assert.IsNull(library.Save("notes", CodeAlta.Orchestration.Runtime.Scripts.CodeModeScriptScope.Global, "return 1;", null, null).Refusal);
        // A script of the project is in every space of its sessions, whatever the configuration says of the name.
        Assert.IsNull(library.Save("local", CodeAlta.Orchestration.Runtime.Scripts.CodeModeScriptScope.Project, "return 2;", folder, null).Refusal);
        var personal = Agent(await fixture.CreateAsync(AltaCallerIdentity.Cli, "--project", project.Id, "--space", "personal").ConfigureAwait(false));
        var business = Agent(await fixture.CreateAsync(AltaCallerIdentity.Cli, "--project", project.Id, "--space", "business").ConfigureAwait(false));
        Task<AltaCommandResult> Invoke(AltaCallerIdentity caller, params string[] args) => fixture.Dispatcher.InvokeAsync(args, caller: caller, cwd: folder).AsTask();
        static string[] Names(AltaCommandResult result) => [.. ReadJsonLines(result.Stdout).Where(static line => line.GetProperty("type").GetString() == "alta.script").Select(static line => line.GetProperty("name").GetString()!)];

        CollectionAssert.AreEquivalent(new[] { "local", "notes" }, Names(await Invoke(personal, "script", "list").ConfigureAwait(false)));
        CollectionAssert.AreEquivalent(new[] { "local", "notes", "review" }, Names(await Invoke(business, "script", "list").ConfigureAwait(false)));
        CollectionAssert.AreEquivalent(new[] { "local", "notes", "review" }, Names(await Invoke(AltaCallerIdentity.Cli, "script", "list").ConfigureAwait(false)), "the user sees every script");
        StringAssert.Contains((await Invoke(personal, "script", "show", "review").ConfigureAwait(false)).Transcript, "script.notFound");
        var refused = await Invoke(personal, "script", "run", "review", "--arg", "pr=7").ConfigureAwait(false);
        StringAssert.Contains(refused.Transcript, "script.runRefused");
        StringAssert.Contains(refused.Transcript, "not available in the space 'personal'");
        var badArgument = await Invoke(business, "script", "run", "review", "--arg", "pr=seven").ConfigureAwait(false);
        StringAssert.Contains(badArgument.Transcript, "the argument `pr` must be integer, not string");

        var ran = await Invoke(business, "script", "run", "review", "--arg", "pr=7").ConfigureAwait(false);

        Assert.AreEqual(AltaExitCodes.Success, ran.ExitCode, ran.Stdout + ran.Stderr);
        var record = ReadJsonLines(ran.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.script.run");
        Assert.AreEqual("""{"pr":7}""", record.GetProperty("arguments").GetString());
        var sessionId = record.GetProperty("sessionId").GetString()!;
        Assert.IsTrue(fixture.Runs!.RequiresCodeMode(sessionId), "the session runs the script through exec, whatever the setting says");
        var space = await fixture.Runtime.GetSessionSpaceAsync(sessionId).ConfigureAwait(false);
        Assert.AreEqual(("business", business.SourceSessionId), (space!.SpaceId, space.ParentSessionId), "a sub-session of the caller, in its space");
    }

    [TestMethod]
    public async Task ASessionThatDoesWithoutTools_CreatesAndForksSessionsThatDoWithoutThemToo_FromAScriptAsDirectly()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync(withOwner: true).ConfigureAwait(false);
        var parent = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        // What `altacli run --disable-tools` gives the session of its run: the sessions it creates take the same patterns.
        fixture.Runtime.SetDisabledToolPatterns(Id(parent), ["shell_command", "web"]);
        var identity = Agent(parent) with { ScriptRunId = "run-narrow" };
        var binding = new CodeAlta.LiveTool.Scripts.AltaScriptBinding(fixture.Dispatcher.Describe(identity, null));
        var direct = await fixture.Dispatcher.InvokeAsync(["session", "create", "--global"], caller: Agent(parent)).ConfigureAwait(false);
        var named = await fixture.Dispatcher.InvokeAsync(["session", "create", "--global", "--prompt-id", "default"], caller: Agent(parent)).ConfigureAwait(false);
        var fork = await fixture.Dispatcher.InvokeAsync(["session", "fork", Id(parent)], caller: Agent(parent)).ConfigureAwait(false);

        var result = await new CodeAlta.CodeMode.CodeModeHost(new CodeAlta.CodeMode.CodeModeApi(binding.Functions), new CodeAlta.CodeMode.CodeModeScriptCache())
            .RunAsync(CodeAlta.CodeMode.CodeModeSource.Prepare("""
                try { const created = await alta.session.create({ global: true }); return { created: created.sessionId }; }
                catch (e) { return { refused: e.code }; }
                """), new CodeAlta.LiveTool.Scripts.AltaScriptCallHandler(fixture.Dispatcher, binding, identity, null)).ConfigureAwait(false);

        Assert.AreEqual(CodeAlta.CodeMode.CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        using var value = JsonDocument.Parse(result.ValueJson!);
        Assert.IsFalse(value.RootElement.TryGetProperty("refused", out var refused), "the script creates it as the model's own call does: " + refused);
        var children = new[]
        {
            CreatedId(direct, "alta.session.created"), CreatedId(named, "alta.session.created"), CreatedId(fork, "alta.session.forked"),
            value.RootElement.GetProperty("created").GetString()!,
        };
        foreach (var child in children)
        {
            var disabled = await fixture.Runtime.GetDisabledToolsAsync(child).ConfigureAwait(false);
            foreach (var tool in new[] { "shell_command", "webget", "web_search", "web_fetch" })
                Assert.IsTrue(disabled.Contains(tool), $"{child} does without {tool}, as the session that created it does");
        }

        static string CreatedId(AltaCommandResult result, string type)
        {
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
            return ReadJsonLines(result.Stdout).Single(line => line.GetProperty("type").GetString() == type).GetProperty("sessionId").GetString()!;
        }
    }

    [TestMethod]
    public async Task SandboxCommands_SayWhatEachProfileAllows_AndASandboxedAgentReadsItsOwnAlone()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync(withOwner: true).ConfigureAwait(false);
        fixture.WriteConfig("""
            [sandbox]
            all_sessions = "workspace"

            [sandbox.profiles.company]
            extends = "workspace"
            hosts = ["pkgs.dev.azure.com"]
            env = ["FEED_TOKEN"]
            mcp_servers = ["docs"]

            [sandbox.profiles.broken]
            extends = "nowhere"
            """);
        async Task<JsonElement[]> Records(AltaCallerIdentity caller, params string[] arguments)
        {
            var result = await fixture.Dispatcher.InvokeAsync(arguments, caller: caller).ConfigureAwait(false);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
            return [.. ReadJsonLines(result.Stdout).Where(static line => line.GetProperty("type").GetString()!.StartsWith("alta.sandbox.", StringComparison.Ordinal))];
        }

        // The user sees every profile, with what it allows and what is wrong with it.
        var listed = await Records(AltaCallerIdentity.Cli, "sandbox", "list").ConfigureAwait(false);
        var profiles = listed.Where(static record => record.GetProperty("type").GetString() == "alta.sandbox.profile").ToDictionary(static record => record.GetProperty("name").GetString()!);
        CollectionAssert.AreEquivalent(new[] { "workspace", "strict", "company", "broken" }, profiles.Keys.ToArray());
        Assert.AreEqual(("hosts", "ask", true), (profiles["workspace"].GetProperty("network").GetString(), profiles["workspace"].GetProperty("outside").GetString(), profiles["workspace"].GetProperty("builtIn").GetBoolean()));
        Assert.AreEqual(("off", "never"), (profiles["strict"].GetProperty("network").GetString(), profiles["strict"].GetProperty("outside").GetString()));
        Assert.AreEqual("workspace", profiles["company"].GetProperty("extends").GetString());
        StringAssert.Contains(profiles["broken"].GetProperty("problem").GetString(), "nowhere");
        Assert.AreEqual("workspace", listed.Single(static record => record.GetProperty("type").GetString() == "alta.sandbox.summary").GetProperty("allSessions").GetString());

        var company = (await Records(AltaCallerIdentity.Cli, "sandbox", "show", "company").ConfigureAwait(false)).Single();
        var hosts = company.GetProperty("hosts").EnumerateArray().Select(static host => host.GetString()).ToArray();
        CollectionAssert.Contains(hosts, "nuget.org", "what it takes from the profile it extends");
        CollectionAssert.Contains(hosts, "pkgs.dev.azure.com");
        Assert.AreEqual("FEED_TOKEN", company.GetProperty("env")[0].GetString(), "the names of the variables, never their values");
        Assert.AreEqual("docs", company.GetProperty("mcpServers")[0].GetString());
        Assert.AreEqual(0, (await Records(AltaCallerIdentity.Cli, "sandbox", "show", "strict").ConfigureAwait(false)).Single().GetProperty("hosts").GetArrayLength());
        AssertSandboxRefused(await fixture.Dispatcher.InvokeAsync(["sandbox", "show", "nowhere"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false), "sandbox.profileNotFound",
            AltaExitCodes.NotFound);
        AssertSandboxRefused(await fixture.Dispatcher.InvokeAsync(["sandbox", "show"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false), "usage.missingProfile", AltaExitCodes.Usage);
        var computer = (await Records(AltaCallerIdentity.Cli, "sandbox", "status").ConfigureAwait(false)).Single();
        Assert.IsTrue(computer.TryGetProperty("available", out _) && !computer.TryGetProperty("sessionId", out _), "without a session: whether a sandbox runs here");

        // A sandboxed agent reads the profile it runs with, and the sandbox of the sessions it reaches.
        var agent = Agent(await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false));
        var unrelated = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        var own = (await Records(agent, "sandbox", "list").ConfigureAwait(false)).Where(static record => record.GetProperty("type").GetString() == "alta.sandbox.profile").ToArray();
        Assert.AreEqual(("workspace", true), (own.Single().GetProperty("name").GetString(), own.Single().GetProperty("current").GetBoolean()), "its own profile alone");
        Assert.AreEqual("workspace", (await Records(agent, "sandbox", "show").ConfigureAwait(false)).Single().GetProperty("name").GetString());
        AssertSandboxRefused(await fixture.Dispatcher.InvokeAsync(["sandbox", "show", "company"], caller: agent).ConfigureAwait(false), "sandbox.profileDenied");
        var status = (await Records(agent, "sandbox", "status").ConfigureAwait(false)).Single();
        Assert.AreEqual((agent.SourceSessionId, true, "workspace", "all_sessions"),
            (status.GetProperty("sessionId").GetString(), status.GetProperty("sandboxed").GetBoolean(), status.GetProperty("profile").GetString(), status.GetProperty("source").GetString()));
        Assert.AreEqual(0, status.GetProperty("refusedHosts").GetArrayLength());
        AssertSandboxRefused(await fixture.Dispatcher.InvokeAsync(["sandbox", "status", "--session", Id(unrelated)], caller: agent).ConfigureAwait(false), "sandbox.notOwnSession");
    }

    private static void AssertSandboxRefused(AltaCommandResult result, string code, int exitCode)
    {
        Assert.AreEqual(exitCode, result.ExitCode, result.Stdout + result.Stderr);
        var error = ReadJsonLines(result.Stdout + result.Stderr).First(static line => line.GetProperty("type").GetString() == "alta.error");
        Assert.AreEqual(code, error.GetProperty("code").GetString(), result.Stdout + result.Stderr);
    }

    private static void AssertSandboxRefused(AltaCommandResult result, string code)
    {
        Assert.AreEqual(AltaExitCodes.PolicyDenied, result.ExitCode, result.Stdout + result.Stderr);
        var error = ReadJsonLines(result.Stdout + result.Stderr).First(static line => line.GetProperty("type").GetString() == "alta.error");
        Assert.AreEqual(code, error.GetProperty("code").GetString(), result.Stdout + result.Stderr);
    }

    [TestMethod]
    public async Task SessionArchive_HidesTheSessionFromTheList_UntilRestored_AndAnAgentArchivesOnlyInItsOwnSpace()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync().ConfigureAwait(false);
        var business = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business").ConfigureAwait(false);
        var personal = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        var other = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        var personalAgent = Agent(personal);

        foreach (var verb in new[] { "archive", "restore" })
            fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", verb, Id(business)], caller: personalAgent).ConfigureAwait(false), "space.notOwnSpace");
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "archive", Id(personal)], caller: personalAgent).ConfigureAwait(false), "session.running");

        var archived = await fixture.Dispatcher.InvokeAsync(["session", "archive", Id(other)], caller: personalAgent).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, archived.ExitCode, archived.Stdout + archived.Stderr);
        using (var record = JsonDocument.Parse(archived.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last()))
        {
            Assert.AreEqual("alta.session.archived", record.RootElement.GetProperty("type").GetString());
            Assert.AreEqual(Id(other), record.RootElement.GetProperty("changed")[0].GetString());
        }

        Assert.AreEqual(SessionViewStatus.Archived, (await fixture.Runtime.ResolveOwnedSessionAsync(Id(other), CancellationToken.None).ConfigureAwait(false))!.Status);

        var restored = await fixture.Dispatcher.InvokeAsync(["session", "restore", Id(other)], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, restored.ExitCode, restored.Stdout + restored.Stderr);
        StringAssert.Contains(restored.Stdout, "alta.session.restored");
        Assert.AreNotEqual(SessionViewStatus.Archived, (await fixture.Runtime.ResolveOwnedSessionAsync(Id(other), CancellationToken.None).ConfigureAwait(false))!.Status);
    }

    private static string Id(JsonElement created) => created.GetProperty("sessionId").GetString()!;

    private static AltaCallerIdentity Agent(JsonElement created) => new() { Kind = "agent", SourceSessionId = Id(created) };

    // One setting of a plugin of a project, which the registry changes for the project it is given.
    private sealed class ProjectPluginSettings : IAltaSettings
    {
        public string? Project { get; set; }

        public List<string?> Writes { get; } = [];

        private AltaSettingDescriptor Poll => new("plugins.builds.poll", "Check every", null, "plugin/project:builds", "Extensions › Builds", null, AltaSettingKinds.Integer, [], null, null,
            JsonDocument.Parse("5").RootElement.Clone(), AltaSettingAccess.ReadWrite, []) { Owner = "plugin", Plugin = "builds", Scopes = ["project"], Project = Project };

        public IReadOnlyList<AltaSettingDescriptor> List() => [Poll];

        public ValueTask<AltaSettingRead> GetAsync(string id, string? projectId, CancellationToken cancellationToken) => new(new AltaSettingRead(AltaSettingStatus.Ok, Poll.Default, "default", null));

        public ValueTask<AltaSettingWrite> SetAsync(string id, JsonElement? value, string? projectId, AltaSettingOrigin origin, CancellationToken cancellationToken)
        {
            Writes.Add(projectId);
            return new(new AltaSettingWrite(AltaSettingStatus.Ok, value, Poll.Default, null));
        }

        public bool Open(string? page, string? id, string? callerSessionId) => false;
    }

    private sealed class SpaceResourceFixture : IAsyncDisposable
    {
        private readonly TempDirectory _root;
        private readonly SessionRuntimeService _runtime;

        private SpaceResourceFixture(TempDirectory root, CatalogOptions options, SpaceCatalog spaces, SessionRuntimeService runtime, AltaCommandDispatcher dispatcher, ShownSpaceView view,
            CodeAlta.Orchestration.Runtime.Scripts.CodeModeRunRegistry? runs)
            => (_root, Options, Spaces, _runtime, Dispatcher, View, Runs) = (root, options, spaces, runtime, dispatcher, view, runs);

        // The runs of the scripts of Code Mode, with the library.
        public CodeAlta.Orchestration.Runtime.Scripts.CodeModeRunRegistry? Runs { get; }

        public CatalogOptions Options { get; }

        public ShownSpaceView View { get; }

        public SessionRuntimeService Runtime => _runtime;

        public SpaceCatalog Spaces { get; }

        public AltaCommandDispatcher Dispatcher { get; }

        public static Task<SpaceResourceFixture> CreateAsync(params string[] moreProviders) => CreateAsync(withOwner: false, moreProviders);

        // withOwner: the command owner of the host, which the commands that change an attached session need.
        public static Task<SpaceResourceFixture> CreateAsync(bool withOwner, params string[] moreProviders) => CreateAsync(withOwner, withLibrary: false, moreProviders);

        // withLibrary: the library of scripts, for `alta script` and the scripts of Code Mode.
        public static Task<SpaceResourceFixture> CreateAsync(bool withOwner, bool withLibrary, params string[] moreProviders) => CreateAsync(withOwner, withLibrary, null, moreProviders);

        // settings: the settings of the window, for `alta settings`.
        public static async Task<SpaceResourceFixture> CreateAsync(bool withOwner, bool withLibrary, IAltaSettings? settings, params string[] moreProviders)
        {
            var root = TempDirectory.Create();
            var options = new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "global") };
            Directory.CreateDirectory(options.GlobalRoot);
            var projectCatalog = new ProjectCatalog(options);
            var spaces = new SpaceCatalog(projectCatalog);
            await spaces.CreateAsync("Business").ConfigureAwait(false);
            await spaces.CreateAsync("Personal").ConfigureAwait(false);
            var descriptor = new ModelProviderDescriptor(new ModelProviderId("alpha"), "Alpha");
            var registry = new ModelProviderRegistry();
            registry.RegisterOrReplaceSessionRuntime(descriptor, () => new StatefulProviderRuntime(descriptor.ProviderId));
            ModelProviderDescriptor[] descriptors = [descriptor, .. moreProviders.Select(static key => new ModelProviderDescriptor(new ModelProviderId(key), key))];
            foreach (var more in descriptors.Skip(1))
            {
                registry.RegisterOrReplaceSessionRuntime(more, () => new StatefulProviderRuntime(more.ProviderId));
            }
            var runtime = CreateRuntime(options, registry);
            var view = new ShownSpaceView { ShownSpaceId = "personal" };
            var services = new AltaServiceCollection();
            if (withOwner) services.Add(new OwnedSessionCommandService(runtime, projectCatalog, options, 8, () => true));
            var runs = withLibrary ? new CodeAlta.Orchestration.Runtime.Scripts.CodeModeRunRegistry(null) : null;
            if (withLibrary) services.Add(new CodeAlta.Orchestration.Runtime.Scripts.CodeModeScriptLibrary(options)).Add(runs!);
            if (settings is not null) services.Add<IAltaSettings>(settings);
            var dispatcher = CreateDispatcher(services
                .Add(options)
                .Add(projectCatalog)
                .Add(spaces)
                .Add<IAltaSpaceView>(view)
                .Add(new SessionViewCatalog(options))
                .Add(runtime)
                .Add(new Catalog.Skills.SkillCatalog())
                .Add<IReadOnlyList<ModelProviderDescriptor>>(descriptors)
                .Add<IAltaSessionQueryService>(new ThrowingSessionQueryService()));
            return new SpaceResourceFixture(root, options, spaces, runtime, dispatcher, view, runs);
        }

        public void WriteConfig(string content) => File.WriteAllText(Path.Combine(Options.GlobalRoot!, "config.toml"), content);

        public async Task WritePromptAsync(string id)
        {
            var folder = Path.Combine(Options.GlobalRoot!, "prompts", "agents");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, id + ".prompt.md"),
                $"---\nname: {id}\ndescription: The {id} agent.\nsystem: default\n---\nThe {id} agent.\n").ConfigureAwait(false);
        }

        public async Task<JsonElement> CreateAsync(AltaCallerIdentity caller, params string[] more)
        {
            var result = await Dispatcher.InvokeAsync(["session", "create", .. more], caller: caller).ConfigureAwait(false);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
            return ReadJsonLines(result.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.session.created");
        }

        public void AssertRefused(AltaCommandResult result, string code)
        {
            Assert.AreEqual(AltaExitCodes.Unsupported, result.ExitCode, result.Stdout + result.Stderr);
            var error = ReadJsonLines(result.Stdout + result.Stderr).First(static line => line.GetProperty("type").GetString() == "alta.error");
            Assert.AreEqual(code, error.GetProperty("code").GetString());
        }

        public async Task<string[]> PromptIdsAsync(AltaCallerIdentity caller)
        {
            var result = await Dispatcher.InvokeAsync(["prompt", "list"], caller: caller).ConfigureAwait(false);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
            return [.. ReadJsonLines(result.Stdout).Where(static line => line.TryGetProperty("promptId", out _)).Select(static line => line.GetProperty("promptId").GetString()!)];
        }

        public async Task<string[]> SkillNamesAsync(AltaCallerIdentity caller)
        {
            var result = await Dispatcher.InvokeAsync(["skill", "list", "--detailed"], caller: caller).ConfigureAwait(false);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
            return [.. ReadJsonLines(result.Stdout).Where(static line => line.TryGetProperty("name", out _)).Select(static line => line.GetProperty("name").GetString()!)];
        }

        public async ValueTask DisposeAsync()
        {
            if (Runs is not null) await Runs.DisposeAsync().ConfigureAwait(false);
            await _runtime.DisposeAsync().ConfigureAwait(false);
            _root.Dispose();
        }
    }
}

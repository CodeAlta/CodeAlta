using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SessionSandboxTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TheSandboxOfASession_IsDecidedByTheMostSpecificScope_AndItsOwnChoiceIsSaved()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("s");
        Assert.IsFalse((await f.Runtime.GetSessionSandboxAsync("s")).IsSandboxed, "nothing sandboxes a session by default");

        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        var all = await f.Runtime.GetSessionSandboxAsync("s");
        Assert.AreEqual(new SandboxChoice("workspace", SandboxScope.AllSessions), all.Choice);
        Assert.AreEqual(SandboxNetwork.Hosts, all.Profile!.Network);

        f.Project.Sandbox = SandboxProfiles.Off;
        await f.Projects.SaveAsync(f.Project);
        Assert.AreEqual(new SandboxChoice(null, SandboxScope.Project), (await f.Runtime.GetSessionSandboxAsync("s")).Choice, "the project turns it off");

        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n\n[providers.codex]\ntype = \"codex\"\nsandbox = \"strict\"\n");
        var provider = await f.Runtime.GetSessionSandboxAsync("s");
        Assert.AreEqual(new SandboxChoice("strict", SandboxScope.Provider), provider.Choice, "distrust of a provider follows it into every project");
        Assert.AreEqual(SandboxNetwork.Off, provider.Profile!.Network);

        var session = (await f.Runtime.ResolveOwnedSessionAsync("s", CancellationToken.None))!;
        await f.Runtime.SetOwnedSandboxAsync(session, SandboxProfiles.Off);
        Assert.AreEqual(new SandboxChoice(null, SandboxScope.Session), (await f.Runtime.GetSessionSandboxAsync("s")).Choice, "the user's choice for the session wins");
        Assert.AreEqual(SandboxProfiles.Off, f.LastStateRecord("s").GetProperty("sandbox").GetString(), "the choice is saved with the session");

        await using var restarted = f.CreateRuntime();
        Assert.AreEqual(new SandboxChoice(null, SandboxScope.Session), (await restarted.GetSessionSandboxAsync("s")).Choice, "and read again after a restart");
    }

    [TestMethod]
    public async Task AConfigurationThatCannotBeRead_KeepsTheSandboxesReadLast()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("s");
        f.WriteConfig("[sandbox]\nall_sessions = \"strict\"\n\n[providers.codex]\ntype = \"codex\"\nsandbox = \"workspace\"\n");
        Assert.AreEqual(new SandboxChoice("workspace", SandboxScope.Provider), (await f.Runtime.GetSessionSandboxAsync("s")).Choice);

        // Broken for a moment, or by a session: the sandbox of the session does not turn off.
        f.WriteConfig("[sandbox\nall_sessions = ");
        Assert.AreEqual(new SandboxChoice("workspace", SandboxScope.Provider), (await f.Runtime.GetSessionSandboxAsync("s")).Choice);
        f.WriteConfig("[sandbox]\nall_sessions = \"strict\"\n");
        Assert.AreEqual(new SandboxChoice("strict", SandboxScope.AllSessions), (await f.Runtime.GetSessionSandboxAsync("s")).Choice, "a configuration read again applies");
    }

    [TestMethod]
    public async Task AProfileThatCannotBeUsed_KeepsTheSessionSandboxed_WithAProblem()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("s");
        f.WriteConfig("[sandbox]\nall_sessions = \"missing\"\n");

        var sandbox = await f.Runtime.GetSessionSandboxAsync("s");

        Assert.IsTrue(sandbox.IsSandboxed, "a sandbox that cannot run never becomes no sandbox");
        Assert.IsNull(sandbox.Profile);
        Assert.Contains("'missing'", sandbox.Problem!);
    }

    [TestMethod]
    public async Task ASessionCreatedByASandboxedSession_GetsItsProfile_WhateverItAsks()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("creator");
        await f.CreateSessionAsync("plain");
        var creator = (await f.Runtime.ResolveOwnedSessionAsync("creator", CancellationToken.None))!;
        await f.Runtime.SetOwnedSandboxAsync(creator, "strict");

        Assert.AreEqual("strict", await f.Runtime.GetCreatedSessionSandboxAsync("creator"));
        Assert.IsNull(await f.Runtime.GetCreatedSessionSandboxAsync("plain"));
        Assert.IsNull(await f.Runtime.GetCreatedSessionSandboxAsync(null), "the user, with no session, decides");
    }

    [TestMethod]
    public async Task TheProfilesOfTheUser_AreMapped_AndAValueNoVersionKnowsIsTheSafestOne()
    {
        await using var f = await Fixture.CreateAsync();
        f.WriteConfig("[sandbox.profiles.mine]\nextends = \"workspace\"\nnetwork = \"everything\"\noutside = \"sometimes\"\nhosts = [\"pkgs.dev.azure.com\"]\n");

        var profile = SandboxProfiles.Resolve("mine", f.Runtime.ReadSandboxSettings().Profiles);

        Assert.AreEqual(SandboxNetwork.Off, profile.Network);
        Assert.AreEqual(SandboxOutside.Never, profile.Outside);
        CollectionAssert.Contains(profile.Hosts.ToList(), "pkgs.dev.azure.com");
    }

    [TestMethod]
    public async Task ARepositoryTable_IsReportedAndIgnored()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("s");
        Directory.CreateDirectory(Path.Combine(f.Project.ProjectPath, ".alta"));
        File.WriteAllText(Path.Combine(f.Project.ProjectPath, ".alta", "config.toml"), "[sandbox]\nall_sessions = \"off\"\n");
        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");

        var sandbox = await f.Runtime.GetSessionSandboxAsync("s");

        Assert.IsTrue(sandbox.ProjectSetsSandbox);
        Assert.AreEqual("workspace", sandbox.Choice.Profile);
    }

    [TestMethod]
    public async Task TheToolsOfASandboxedSession_RunInItsWorker_WhichFollowsTheProfileOfTheMoment()
    {
        var backend = new InProcessBackend();
        await using var f = await Fixture.CreateAsync(backend);
        await f.CreateSessionAsync("s");
        var tools = f.RoutedTools("s");

        var plain = await tools.CallAsync("write_file", new { path = "plain.txt", content = "here" });
        Assert.IsTrue(plain.Success, plain.Error);
        Assert.AreEqual(0, backend.Starts.Count, "a session that is not sandboxed runs its tools here");

        f.WriteConfig("[sandbox]\nall_sessions = \"strict\"\n");
        var written = await tools.CallAsync("write_file", new { path = "boxed.txt", content = "in the box" });
        Assert.IsTrue(written.Success, written.Error);
        Assert.AreEqual("in the box", File.ReadAllText(Path.Combine(f.Project.ProjectPath, "boxed.txt")));
        var read = await tools.CallAsync("read_file", new { path = "boxed.txt" });
        Assert.IsTrue(read.Success, read.Error);
        Assert.AreEqual(1, backend.Starts.Count, "one worker per session, kept between calls");
        var policy = backend.Starts[0];
        Assert.AreEqual("strict", policy.ProfileName);
        Assert.AreEqual(Path.GetFullPath(f.Project.ProjectPath), policy.WorkingDirectory);

        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        Assert.IsTrue((await tools.CallAsync("read_file", new { path = "boxed.txt" })).Success);
        Assert.AreEqual(2, backend.Starts.Count, "another profile is another container");
        Assert.AreEqual("workspace", backend.Starts[1].ProfileName);

        await f.Runtime.SandboxWorkers.StopIfUnusedAsync("s");
        Assert.IsTrue((await tools.CallAsync("read_file", new { path = "boxed.txt" })).Success);
        Assert.AreEqual(3, backend.Starts.Count, "a session that went away left no worker: the next call starts one");

        f.WriteConfig("[sandbox]\nall_sessions = \"missing\"\n");
        var refused = await tools.CallAsync("read_file", new { path = "boxed.txt" });
        Assert.IsFalse(refused.Success, "a profile that cannot be used runs nothing, here or there");
        Assert.Contains("'missing'", refused.Error!);
    }

    [TestMethod]
    public async Task ASandboxThatCannotRun_RefusesTheRunAndTheTools_AndSaysWhy()
    {
        var backend = new InProcessBackend { Available = false };
        await using var f = await Fixture.CreateAsync(backend);
        await f.CreateSessionAsync("s");
        var router = f.Runtime.CreateSandboxToolRouter("s", "codex", [f.Project.ProjectPath]);
        Assert.IsNull(await router.RefuseRunAsync(providerRunsItsOwnTools: true, CancellationToken.None), "nothing is refused to a session that is not sandboxed");

        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        Assert.Contains("not available on this computer", (await router.RefuseRunAsync(false, CancellationToken.None))!);
        backend.Available = true;
        Assert.IsNull(await router.RefuseRunAsync(false, CancellationToken.None));
        Assert.Contains("runs its own tools", (await router.RefuseRunAsync(true, CancellationToken.None))!);

        backend.Fails = true;
        var result = await f.RoutedTools("s").CallAsync("shell_command", new { command = "echo no" });
        Assert.IsFalse(result.Success);
        Assert.Contains("did not run", result.Error!);
        Assert.Contains("refused by the test", result.Error!);

        var (starter, unused, refusal) = await f.Runtime.GetJobStarterAsync("s");
        Assert.IsNull(starter);
        Assert.IsNull(unused);
        Assert.Contains("not attached", refusal!, "a background command waits for a session that runs");
    }

    [TestMethod]
    public void ABackgroundCommandOfAGlobalSession_StartsInTheFolderItsSandboxGivesIt()
    {
        // A global session works in the folder of CodeAlta, which its sandbox does not grant: it works in one of its own.
        var alta = Path.Combine(Path.GetTempPath(), "home", ".alta");
        var work = Path.Combine(Path.GetTempPath(), "sandbox", "sessions", "g", "work");

        Assert.AreEqual(work, SessionRuntimeService.JobFolder(alta + Path.DirectorySeparatorChar, alta, work));
        var project = Path.Combine(Path.GetTempPath(), "project");
        Assert.AreEqual(project, SessionRuntimeService.JobFolder(project, alta, work), "a folder the command names stays");
        Assert.AreEqual(project, SessionRuntimeService.JobFolder(project, project, project), "the folder of a project session is its sandbox's");
    }

    [TestMethod]
    public async Task ACommandOutsideTheSandbox_IsAskedWithItsReason_AndRunsHereOnlyWhenAllowed()
    {
        var backend = new InProcessBackend();
        await using var f = await Fixture.CreateAsync(backend);
        await f.CreateSessionAsync("s");
        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        var asked = new List<AgentCommandPermissionRequest>();
        var answer = AgentPermissionDecisionKind.Deny;
        var router = f.Runtime.CreateSandboxToolRouter("s", "codex", [f.Project.ProjectPath]);
        var tools = f.RoutedTools("s", router, (request, _) =>
        {
            if (request is AgentCommandPermissionRequest command) lock (asked) asked.Add(command);
            return Task.FromResult(new AgentPermissionDecision(request is AgentCommandPermissionRequest { OutsideSandbox: true } ? answer : AgentPermissionDecisionKind.AllowOnce));
        });
        Assert.IsNull(await router.RefuseRunAsync(false, CancellationToken.None));
        // The parameter is offered to a profile that lets commands out, from the run on.
        tools = f.RoutedTools("s", router, tools.Permissions);
        Assert.IsTrue(tools.Spec("shell_command").InputSchema.GetProperty("properties").TryGetProperty("outside_sandbox", out _));

        var refused = await tools.CallAsync("shell_command", new { command = "echo out", outside_sandbox = "It needs the NuGet API key." });
        Assert.IsFalse(refused.Success);
        Assert.Contains("did not allow", refused.Error!);
        var request = asked.Single(static request => request.OutsideSandbox);
        Assert.AreEqual(("echo out", "It needs the NuGet API key."), (request.Command, request.Reason));
        Assert.AreEqual(0, backend.Starts.Count, "nothing ran, in the sandbox or out of it");

        answer = AgentPermissionDecisionKind.AllowOnce;
        var allowed = await tools.CallAsync("shell_command", new { command = "echo out", outside_sandbox = "It needs the NuGet API key." });
        Assert.IsTrue(allowed.Success, allowed.Error);
        Assert.StartsWith("exit_code: 0", RoutedToolSet.Text(allowed), "the output keeps the form the timelines read");
        Assert.HasCount(1, allowed.Items, "the note is a line of the text the timelines show, not a part they leave out");
        Assert.EndsWith("\n[sandbox: workspace] The command ran outside the sandbox, as the user allowed.", ((AgentToolResultItem.Text)allowed.Items[0]).Value.Replace("\r\n", "\n"));
        Assert.Contains("ran outside the sandbox", RoutedToolSet.Text(allowed));
        Assert.Contains("out", RoutedToolSet.Text(allowed));
        Assert.AreEqual(0, backend.Starts.Count, "it ran here");
        Assert.HasCount(2, asked.Where(static request => request.OutsideSandbox), "the shell of the host does not ask it a second time");

        // A model whose functions have every parameter required sends an empty reason for a command that stays inside.
        var inside = await tools.CallAsync("shell_command", new { command = "echo in", outside_sandbox = "" });
        Assert.IsTrue(inside.Success, inside.Error);
        Assert.HasCount(2, asked.Where(static request => request.OutsideSandbox), "nobody is asked for it");
        Assert.HasCount(1, backend.Starts, "it runs in the sandbox");
        f.WriteConfig("[sandbox]\nall_sessions = \"strict\"\n");
        Assert.IsNull(await router.RefuseRunAsync(false, CancellationToken.None));
        tools = f.RoutedTools("s", router, tools.Permissions);
        Assert.IsFalse(tools.Spec("shell_command").InputSchema.GetProperty("properties").TryGetProperty("outside_sandbox", out _), "a profile that lets no command out offers nothing");
        var never = await tools.CallAsync("shell_command", new { command = "echo out", outside_sandbox = "Please." });
        Assert.Contains("runs no command outside", never.Error!);
    }

    [TestMethod]
    public async Task TheAgentIsToldWhatItsSandboxAllows_OnceAndAgainWhenItChanges()
    {
        await using var f = await Fixture.CreateAsync(new InProcessBackend());
        await f.CreateSessionAsync("s");
        Assert.AreEqual("off", CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.Note(await f.Runtime.GetSessionSandboxAsync("s"), null).Noted);

        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        var workspace = CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.Note(await f.Runtime.GetSessionSandboxAsync("s"), "C:/scratch/s");
        Assert.Contains("profile `workspace`", workspace.Text);
        Assert.Contains("nuget.org", workspace.Text);
        Assert.Contains("`outside_sandbox", workspace.Text);
        Assert.Contains("C:/scratch/s", workspace.Text);

        f.WriteConfig("[sandbox]\nall_sessions = \"strict\"\n");
        var strict = CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.Note(await f.Runtime.GetSessionSandboxAsync("s"), null);
        Assert.Contains("have no network", strict.Text);
        Assert.Contains("No command runs outside", strict.Text);
        Assert.AreNotEqual(workspace.Noted, strict.Noted, "another sandbox is told again");
        var sandbox = await f.Runtime.GetSessionSandboxAsync("s");
        Assert.Contains("`-m:1`", CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.Note(sandbox, null, windows: true).Text);
        Assert.DoesNotContain("`-m:1`", CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.Note(sandbox, null, windows: false).Text);
    }

    [TestMethod]
    public void ACommandTheSandboxBlocked_GetsANoteThatSaysWhat()
    {
        var profile = SandboxProfiles.Resolve("workspace", []);
        var failed = new AgentToolResult(false, [new AgentToolResultItem.Text("error: could not lock config file C:/code/Lib/.git/config: Permission denied")], "shell_command exited with code 255.");

        Assert.Contains("writes only in the folders of the session", CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.FailureNote(profile, failed, [], [])!);
        var network = CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.FailureNote(profile,
            new AgentToolResult(false, [new AgentToolResultItem.Text("Unable to load the service index")], null), ["a.org:443"], ["a.org:443", "api.example.com:443"])!;
        Assert.Contains("refused api.example.com:443", network);
        Assert.DoesNotContain("a.org", network, "only what this command was refused");
        Assert.IsNull(CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.FailureNote(profile, new AgentToolResult(false, [new AgentToolResultItem.Text("1 test failed")], null), [], []),
            "a failure the sandbox did not cause gets no note");

        var socket = new AgentToolResult(false, [new AgentToolResultItem.Text("error: NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.\n" +
            "error:   An attempt was made to access a socket in a way forbidden by its access permissions. (api.nuget.org:443)")], null);
        Assert.Contains("has no network", CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.FailureNote(SandboxProfiles.Resolve("strict", []), socket, [], [])!);
        Assert.Contains("through its proxy", CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.FailureNote(profile, socket, [], [])!,
            "a program that does not use the proxy of a profile with hosts");
    }

    [TestMethod]
    public void WhatTheWindowsContainerKeepsFromBuildsAndTests_IsToldAfterTheCommand()
    {
        var workspace = SandboxProfiles.Resolve("workspace", []);
        var nodes = new AgentToolResult(false, [new AgentToolResultItem.Text("exit_code: 1\nstdout:\nBuild FAILED.\n    0 Warning(s)\n    0 Error(s)\n")], "shell_command exited with code 1.");
        StringAssert.Contains(CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.FailureNote(workspace, nodes, [], [], windows: true), "`-m:1`");
        Assert.IsNull(CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.FailureNote(workspace, nodes, [], [], windows: false), "elsewhere the nodes start");
        var errors = new AgentToolResult(false, [new AgentToolResultItem.Text("Build FAILED.\n    0 Warning(s)\n    3 Error(s)\n")], null);
        Assert.IsNull(CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.FailureNote(workspace, errors, [], [], windows: true), "a build with errors failed by itself");

        var vstest = new AgentToolResult(false, [new AgentToolResultItem.Text("vstest.console process failed to connect to testhost process after 90 seconds.\nTest Run Aborted.")], null);
        StringAssert.Contains(CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.FailureNote(workspace, vstest, [], [], windows: true), "`outside_sandbox`");
        Assert.IsNull(CodeAlta.Orchestration.Runtime.Sandbox.SandboxOutsideCommands.FailureNote(SandboxProfiles.Resolve("strict", []), vstest, [], [], windows: true),
            "without network, the processes of the container connect to each other");
    }

    [TestMethod]
    public async Task TheCheckOfASandbox_SaysEachStep_AndStopsAtTheFirstThatCannotGoOn()
    {
        var backend = new InProcessBackend { Available = false };
        await using var f = await Fixture.CreateAsync(backend);
        async Task<List<SandboxCheckStep>> CheckAsync(string profile)
        {
            var steps = new List<SandboxCheckStep>();
            await foreach (var step in f.Runtime.CheckSandboxAsync(profile).WithCancellation(CancellationToken.None)) steps.Add(step);
            return steps;
        }

        var unavailable = (await CheckAsync("strict")).Single();
        Assert.AreEqual(("available", false), (unavailable.Id, unavailable.Passed));
        Assert.Contains("the test has none", unavailable.Detail);

        backend.Available = true;
        var unknown = await CheckAsync("missing");
        Assert.AreEqual("profile", unknown[^1].Id);
        Assert.IsFalse(unknown[^1].Passed);

        // The worker of this test runs here, without a container: what it checks is that every step is run and told.
        var steps = await CheckAsync("strict");
        CollectionAssert.AreEqual(new[] { "available", "profile", "start", "dotnet", "git", "write", "secrets" }, steps.Select(static step => step.Id).ToArray(),
            "a profile without network checks none");
        Assert.IsTrue(steps.Take(4).All(static step => step.Passed), string.Join("\n", steps));
        Assert.HasCount(1, backend.Starts);
        Assert.AreEqual("strict", backend.Starts[0].ProfileName);
    }

    [TestMethod]
    public async Task ClaudeCode_StartsItsProcessInTheSandbox_WithItsAccountAndTheHostsOfAnthropicOnly()
    {
        var backend = new InProcessBackend();
        await using var f = await Fixture.CreateAsync(backend, registry =>
            registry.RegisterOrReplace(new ModelProviderDescriptor(new ModelProviderId("claude"), "Claude", "claude-code"), () => throw new InvalidOperationException("Not started.")));
        await f.CreateSessionAsync("s");
        f.WriteConfig("[sandbox]\nall_sessions = \"strict\"\n");

        var other = f.Runtime.CreateSandboxToolRouter("s", "codex", [f.Project.ProjectPath]);
        Assert.Contains("cannot start", (await other.RefuseRunAsync(providerRunsItsOwnTools: true, CancellationToken.None))!, "a provider whose process the sandbox cannot start");
        Assert.IsNull(other.ProcessHost);

        var router = f.Runtime.CreateSandboxToolRouter("s", "claude", [f.Project.ProjectPath]);
        Assert.IsNull(await router.RefuseRunAsync(providerRunsItsOwnTools: true, CancellationToken.None));
        var host = router.ProcessHost!;
        var account = Path.Combine(f.Project.ProjectPath, "..", "claude-account");
        var launch = new CodeAlta.Agent.Runtime.AgentProcessLaunch("C:/tools/claude.exe", ["-p", "{\"a\":1}"], f.Project.ProjectPath,
            new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = account, ["CLAUDE_GONE"] = null, ["AGENT_SIGN_IN_TOKEN"] = "from-the-provider" })
        {
            ReadOnlyFolders = ["C:/tools"], ReadWriteFolders = [account], Hosts = ["api.anthropic.com"], PassedVariables = ["ANTHROPIC_MODEL", "AGENT_SIGN_IN_TOKEN"],
            ProtectedPaths = [Path.Combine(account, "settings.json")],
        };
        await using (await host.StartAsync(launch, CancellationToken.None))
        {
        }

        var policy = backend.Starts.Single();
        Assert.AreEqual(SandboxNetwork.Hosts, policy.Network, "strict has no network: the agent gets the service of its vendor alone");
        CollectionAssert.AreEqual(new[] { "api.anthropic.com" }, policy.Hosts.ToArray());
        Assert.IsTrue(policy.ReadWrite.Any(folder => string.Equals(folder, Path.GetFullPath(account), StringComparison.OrdinalIgnoreCase)), "its account is written");
        Assert.IsTrue(policy.Protected.Any(path => string.Equals(path, Path.GetFullPath(Path.Combine(account, "settings.json")), StringComparison.OrdinalIgnoreCase)),
            "but not the settings that run hooks later");
        Assert.IsTrue(policy.ReadOnly.Any(static folder => folder.Replace('\\', '/').EndsWith("C:/tools", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(account, policy.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.AreEqual("from-the-provider", policy.Environment["AGENT_SIGN_IN_TOKEN"], "the sign-in the launch gives passes, though its name says a secret");
        Assert.IsFalse(policy.Environment.ContainsKey("CLAUDE_GONE"));

        // Another sandbox is another process: the key changes with the profile.
        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        Assert.IsNull(await router.RefuseRunAsync(providerRunsItsOwnTools: true, CancellationToken.None));
        Assert.AreNotEqual(host.Key, router.ProcessHost!.Key);
        f.WriteConfig("");
        Assert.IsNull(await router.RefuseRunAsync(providerRunsItsOwnTools: true, CancellationToken.None));
        Assert.IsNull(router.ProcessHost, "out of the sandbox, the process is a child of CodeAlta again");
    }

    [TestMethod]
    public async Task AnAcpAgent_StartsInTheSandbox_InAHomeOfItsOwn_AndReachesThePortOfTheToolsOfCodeAlta()
    {
        var backend = new InProcessBackend();
        await using var f = await Fixture.CreateAsync(backend, registry =>
            registry.RegisterOrReplace(new ModelProviderDescriptor(new ModelProviderId("grok"), "Grok Build", "acp"), () => throw new InvalidOperationException("Not started.")));
        await f.CreateSessionAsync("s");
        f.WriteConfig("[sandbox]\nall_sessions = \"strict\"\n");

        var router = f.Runtime.CreateSandboxToolRouter("s", "grok", [f.Project.ProjectPath]);
        Assert.IsNull(await router.RefuseRunAsync(providerRunsItsOwnTools: true, CancellationToken.None));
        var launch = new CodeAlta.Agent.Runtime.AgentProcessLaunch("C:/tools/grok.exe", ["agent", "stdio"], f.Project.ProjectPath,
            new Dictionary<string, string?> { ["XAI_API_KEY"] = "from-the-provider" })
        {
            Hosts = ["api.x.ai"], PassedVariables = ["XAI_API_KEY"], LocalPorts = [50123], OwnHome = "acp-grok",
        };
        await using (await router.ProcessHost!.StartAsync(launch, CancellationToken.None))
        {
        }

        var policy = backend.Starts.Single();
        Assert.AreEqual("home", Path.GetFileName(policy.Home));
        Assert.AreEqual("acp-grok", Path.GetFileName(Path.GetDirectoryName(policy.Home)), "the home of its provider, kept between sessions");
        Assert.AreEqual(policy.Home, policy.Environment["HOME"]);
        CollectionAssert.Contains(policy.LocalPorts.ToArray(), 50123, "its proxy lets it reach the tools of CodeAlta");
        Assert.AreEqual("from-the-provider", policy.Environment["XAI_API_KEY"]);
    }

    [TestMethod]
    public async Task TheGuardOfCommands_IsCheckedByCodeAlta_BeforeTheWorker()
    {
        var backend = new InProcessBackend();
        await using var f = await Fixture.CreateAsync(backend);
        await f.CreateSessionAsync("s");
        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        var tools = f.RoutedTools("s", f.Runtime.CreateSandboxToolRouter("s", "codex", [f.Project.ProjectPath]),
            (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            (command, _, _) => ValueTask.FromResult(command.Contains("commit", StringComparison.Ordinal) ? "Review by the user comes first." : null));

        // The worker is in the sandbox, and one a background command started checks no command: the guard is not its.
        var refused = await tools.CallAsync("shell_command", new { command = "git commit -m change" });

        Assert.IsFalse(refused.Success);
        Assert.AreEqual("Review by the user comes first.", refused.Error);
        Assert.IsEmpty(backend.Starts, "no worker was asked");
    }

    [TestMethod]
    public async Task WebGetOfASandboxWithoutNetwork_IsRefused_BeforeAnyRequest()
    {
        await using var f = await Fixture.CreateAsync(new InProcessBackend());
        await f.CreateSessionAsync("s");
        f.WriteConfig("[sandbox]\nall_sessions = \"strict\"\n");

        var result = await f.RoutedTools("s").CallAsync("webget", new { url = "http://127.0.0.1:9/" });

        Assert.IsFalse(result.Success);
        Assert.Contains("has no network", result.Error!);
    }

    // The tools of a session as its agent gets them, through the router of the runtime.
    private sealed class RoutedToolSet(IReadOnlyList<AgentToolDefinition> tools, string sessionId, AgentPermissionRequestHandler permissions)
    {
        public AgentPermissionRequestHandler Permissions => permissions;

        public AgentToolSpec Spec(string tool) => tools.Single(candidate => candidate.Spec.Name == tool).Spec;

        public static string Text(AgentToolResult result) => string.Concat(result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));

        public async Task<AgentToolResult> CallAsync(string tool, object arguments)
            => await tools.Single(candidate => candidate.Spec.Name == tool).Handler(
                new AgentToolInvocation(new ModelProviderId("codex"), sessionId, Guid.NewGuid().ToString("N"), tool, System.Text.Json.JsonSerializer.SerializeToElement(arguments)),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
    }

    // A sandbox that runs the worker in this process, on two pipes, and records the policies it is given.
    private sealed class InProcessBackend : ISandboxBackend
    {
        public List<SandboxPolicy> Starts { get; } = [];

        public bool Available { get; set; } = true;

        public bool Fails { get; set; }

        public SandboxAvailability GetAvailability() => Available
            ? new SandboxAvailability(true, "test", null, null, new HashSet<SandboxNetwork> { SandboxNetwork.Off, SandboxNetwork.Hosts, SandboxNetwork.On })
            : SandboxAvailability.Unavailable("test", "the test has none.", null);

        public Task<ISandboxProcess> StartAsync(SandboxPolicy policy, string commandLine, string? proxyEndpoint, CancellationToken cancellationToken)
        {
            if (Fails) throw new SandboxUnavailableException("refused by the test.");
            lock (Starts) Starts.Add(policy);
            return Task.FromResult<ISandboxProcess>(new Process());
        }

        private sealed class Process : ISandboxProcess
        {
            private readonly System.IO.Pipes.AnonymousPipeServerStream _toWorker = new(System.IO.Pipes.PipeDirection.Out);
            private readonly System.IO.Pipes.AnonymousPipeServerStream _fromWorker = new(System.IO.Pipes.PipeDirection.Out);
            private readonly System.IO.Pipes.AnonymousPipeClientStream _workerInput;
            private readonly System.IO.Pipes.AnonymousPipeClientStream _hostInput;
            private readonly CancellationTokenSource _kill = new();
            private readonly Task _worker;

            public Process()
            {
                _workerInput = new(System.IO.Pipes.PipeDirection.In, _toWorker.ClientSafePipeHandle);
                _hostInput = new(System.IO.Pipes.PipeDirection.In, _fromWorker.ClientSafePipeHandle);
                _worker = Task.Run(() => CodeAlta.Sandbox.Worker.SandboxWorker.RunAsync(_workerInput, _fromWorker, _kill.Token));
            }

            public int Id => Environment.ProcessId;

            public Stream StandardInput => _toWorker;

            public Stream StandardOutput => _hostInput;

            public Stream StandardError { get; } = new MemoryStream();

            public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
            {
                try
                {
                    await _worker.WaitAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                }

                return 0;
            }

            public void Kill()
            {
                _kill.Cancel();
                _toWorker.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                Kill();
                await WaitForExitAsync(CancellationToken.None);
                _fromWorker.Dispose();
                _workerInput.Dispose();
                _hostInput.Dispose();
                _kill.Dispose();
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _temp;
        private readonly AgentHub _hub;

        private Fixture(string temp, AgentHub hub, SessionRuntimeService runtime, SessionViewJournalStore journal, ProjectCatalog projects, ProjectDescriptor project, CatalogOptions options)
        {
            _temp = temp;
            _hub = hub;
            Runtime = runtime;
            Journal = journal;
            Projects = projects;
            Project = project;
            Options = options;
        }

        public SessionRuntimeService Runtime { get; }

        public SessionViewJournalStore Journal { get; }

        public ProjectCatalog Projects { get; }

        public ProjectDescriptor Project { get; }

        public CatalogOptions Options { get; }

        public static async Task<Fixture> CreateAsync(ISandboxBackend? backend = null, Action<ModelProviderRegistry>? providers = null)
        {
            var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-sandbox-" + Guid.NewGuid().ToString("N"))).FullName;
            var registry = new ModelProviderRegistry();
            providers?.Invoke(registry);
            var hub = new AgentHub(registry, temp);
            var options = new CatalogOptions { GlobalRoot = Path.Combine(temp, "alta") };
            var sessionViewCatalog = new SessionViewCatalog(options);
            var projects = new ProjectCatalog(options);
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(temp, "project")).FullName);
            var runtime = new SessionRuntimeService(hub, new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore()), projects,
                sessionViewCatalog, new AgentInstructionTemplateProvider(catalogOptions: options), options)
            {
                SandboxBackend = backend,
                SandboxDataRoot = Path.Combine(temp, "sandbox"),
                ScratchpadRoot = Path.Combine(temp, "scratchpad"),
            };
            return new Fixture(temp, hub, runtime, sessionViewCatalog.JournalStore, projects, project, options);
        }

        public RoutedToolSet RoutedTools(string sessionId)
            => RoutedTools(sessionId, Runtime.CreateSandboxToolRouter(sessionId, "codex", [Project.ProjectPath]),
                (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)));

        public RoutedToolSet RoutedTools(string sessionId, CodeAlta.Agent.Runtime.Tools.IAgentBuiltInToolRouter router, AgentPermissionRequestHandler permissions,
            Func<string, string, CancellationToken, ValueTask<string?>>? guard = null)
        {
            var options = new CodeAlta.Agent.Runtime.Tools.AgentBuiltInToolOptions
            {
                ProviderId = new ModelProviderId("codex"),
                SessionId = sessionId,
                WorkingDirectory = Project.ProjectPath,
                OnPermissionRequest = permissions,
                CommandGuard = guard is null ? null : (command, folder, token) => guard(command, folder, token),
            };
            return new RoutedToolSet(router.Route(options, CodeAlta.Agent.Runtime.Tools.AgentBuiltInToolFactory.CreateDefaultTools(options)), sessionId, permissions);
        }

        public SessionRuntimeService CreateRuntime()
        {
            var sessionViewCatalog = new SessionViewCatalog(Options);
            return new SessionRuntimeService(_hub, new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore()), new ProjectCatalog(Options),
                sessionViewCatalog, new AgentInstructionTemplateProvider(catalogOptions: Options), Options);
        }

        public void WriteConfig(string toml)
        {
            Directory.CreateDirectory(Options.GlobalRoot);
            File.WriteAllText(Path.Combine(Options.GlobalRoot, "config.toml"), toml);
        }

        public System.Text.Json.JsonElement LastStateRecord(string sessionId)
        {
            var path = new AgentRuntimePathLayout(Options.StateRoot).GetSessionFilePath(sessionId, CreatedAt);
            var line = File.ReadAllLines(path).Last(static line => line.Contains(SessionViewJournalStore.SessionStateEventType, StringComparison.Ordinal));
            using var document = System.Text.Json.JsonDocument.Parse(line);
            return document.RootElement.GetProperty("raw").Clone();
        }

        public async Task CreateSessionAsync(string id)
        {
            var session = new SessionViewDescriptor
            {
                SessionId = id, Kind = SessionViewKind.ProjectSession, ProviderId = "codex", ProviderKey = "codex", ProjectRef = Project.Id,
                WorkingDirectory = Project.ProjectPath, Title = id, Status = SessionViewStatus.Active, CreatedAt = CreatedAt, UpdatedAt = CreatedAt, LastActiveAt = CreatedAt,
            };
            await Journal.EnsureHeaderAsync(session);
            await Journal.CreateSessionStore().UpsertSessionAsync(new() { SessionId = id, ProviderId = new("codex"), ProviderKey = "codex", ProtocolFamily = "codex",
                WorkingDirectory = Project.ProjectPath, Title = id, CreatedAt = CreatedAt, UpdatedAt = CreatedAt });
            await Journal.AppendStateAsync(session, new SessionViewLocalState { ProviderKey = "codex" });
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await _hub.DisposeAsync();
            try
            {
                Directory.Delete(_temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

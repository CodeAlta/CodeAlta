using System.Text.Json;
using CodeAlta.Catalog;

namespace CodeAlta.Cli.Tests;

/// <summary>Where a run writes, per storage mode, and the configuration it reads.</summary>
[TestClass]
public sealed class CliStorageTests
{
    private const string Plain = "--no-plugins";

    [TestMethod]
    public void TheLogFile_IsSharedByRunsAtTheSameTime()
    {
        // A second run on the same state root starts while the first one has the log open, which it must not archive.
        using var root = new CliTestRoot();
        var path = Path.Combine(root.Profile, "logs", CliLogging.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "an earlier run\n");

        using var first = CliLogging.CreateFileWriter(path);
        using var second = CliLogging.CreateFileWriter(path);

        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path)!).Length, "The log is not archived at the start of a run.");
    }

    [TestMethod]
    public async Task Visible_WritesTheJournalAndAMarker_AndNothingElseUnderTheProfile()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        var sessionId = SessionIdOf(run);
        var files = CliTestRoot.Files(root.Profile);
        var journal = files.Single(file => file.EndsWith("/" + sessionId + ".jsonl", StringComparison.Ordinal));
        StringAssert.StartsWith(journal, "sessions/");
        Assert.IsTrue(files.All(static file => file.StartsWith("sessions/", StringComparison.Ordinal)), "Only sessions: " + string.Join(", ", files));
        Assert.IsFalse(files.Any(static file => file.EndsWith("alta.sqlite3", StringComparison.Ordinal)), "No application database of the profile.");
        Assert.IsFalse(File.Exists(Path.Combine(root.Profile, "alta.lock")));
        Assert.IsFalse(File.Exists(Path.Combine(root.Profile, "config.toml")), "The configuration is not written.");
        Assert.IsFalse(File.Exists(Path.Combine(root.Profile, "AGENTS.md")));
        Assert.IsFalse(Directory.Exists(Path.Combine(root.ProjectRoot, ".alta")), "No project state is written.");

        var marker = new SessionMarkerStore(new CatalogOptions { GlobalRoot = root.Profile }).TryRead(sessionId);
        Assert.IsNotNull(marker, "The new session is announced to Desktop and TUI.");
        Assert.AreEqual(journal["sessions/".Length..], marker.JournalPath);
        Assert.AreEqual(SessionLeaseOwner.CliKind, marker.Writer);
        Assert.AreEqual(0, CliTestRoot.Files(Path.Combine(root.Profile, "sessions", "leases")).Length, "The lease is released.");
        Assert.AreEqual(0, CliTestRoot.Files(Path.Combine(root.TempRoot, PrivateApplicationDatabase.DefaultFolderName)).Length, "The private database is deleted.");
    }

    [TestMethod]
    public async Task Visible_WithTheBuiltInPlugins_WritesOnlySessionsAndCachesUnderTheProfile()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var run = await root.RunAsync(provider, ["run", "--format", "jsonl", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        var files = CliTestRoot.Files(root.Profile);
        var others = files.Where(static file => !file.StartsWith("sessions/", StringComparison.Ordinal) && !file.StartsWith("cache/", StringComparison.Ordinal)
            && !file.StartsWith("auth/", StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEqual(Array.Empty<string>(), others, "Written under the profile: " + string.Join(", ", files));
        CollectionAssert.IsSubsetOf(new[] { "Git", "MCP", "ACP" }, root.LastPlugins, "The built-in plugins that declare the command-line application: " + string.Join(", ", root.LastPlugins));
    }

    [TestMethod]
    public async Task Visible_Resume_ContinuesTheSession_AndWritesItsMarkerAgain()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var first = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "hello"]);
        var sessionId = SessionIdOf(first);
        var markers = new SessionMarkerStore(new CatalogOptions { GlobalRoot = root.Profile });
        Assert.IsTrue(markers.Delete(sessionId), "Desktop lists it and deletes the marker.");
        provider.Turns.Enqueue(FakeProvider.Reply("Again."));

        var second = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--resume", sessionId, "again"]);

        Assert.AreEqual(CliExitCodes.Completed, second.ExitCode, second.Stderr);
        var header = JsonDocument.Parse(second.Lines[0]).RootElement.GetProperty("params");
        Assert.AreEqual(sessionId, header.GetProperty("sessionId").GetString());
        Assert.IsFalse(header.GetProperty("created").GetBoolean());
        Assert.AreEqual("Again.", CliRunTests.Result(second).GetProperty("finalMessage").GetString());
        // A run that ends between two looks of Desktop leaves no lease to find: the marker tells it the session changed.
        Assert.IsNotNull(markers.TryRead(sessionId), "A resumed session is announced again.");
    }

    [TestMethod]
    public async Task Visible_WritesAMarkerForEveryChildSessionOfTheRun()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var childId = Guid.CreateVersion7().ToString();
        provider.Script(childId, FakeProvider.Reply("Child result."));
        provider.Turns.Enqueue(FakeProvider.DoThenReply(async request =>
        {
            var host = root.LastHost!;
            var options = new CodeAlta.Orchestration.Runtime.SessionExecutionOptions
            {
                ProviderId = provider.Descriptor.ProviderId,
                ProviderKey = provider.Descriptor.ProviderId.Value,
                Model = provider.Model,
                WorkingDirectory = root.ProjectRoot,
                ProjectRoots = [root.ProjectRoot],
                OnPermissionRequest = static (_, _) => Task.FromResult(new CodeAlta.Agent.AgentPermissionDecision(CodeAlta.Agent.AgentPermissionDecisionKind.AllowOnce)),
            };
            var child = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Child work", request.SessionId, null, null, null, childId);
            _ = host.RuntimeService.SendAsync(child, options, new CodeAlta.Agent.AgentSendOptions { Input = CodeAlta.Agent.AgentInput.Text("child work") });
        }, "Started a child."));
        provider.Turns.Enqueue(FakeProvider.Reply("The child said it."));

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "delegate"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        var markers = new SessionMarkerStore(new CatalogOptions { GlobalRoot = root.Profile });
        Assert.IsNotNull(markers.TryRead(SessionIdOf(run)));
        var child = markers.TryRead(childId);
        Assert.IsNotNull(child, "The child is announced to Desktop and TUI, which may be closed while it runs.");
        StringAssert.EndsWith(child.JournalPath, childId + ".jsonl");
    }

    [TestMethod]
    public async Task Isolated_KeepsTheSessionsInCli_WithoutMarkers()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var run = await root.RunAsync(provider, ["run", Plain, "--isolated", "--format", "jsonl", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        Assert.AreEqual("isolated", JsonDocument.Parse(run.Lines[0]).RootElement.GetProperty("params").GetProperty("storage").GetString());
        var files = CliTestRoot.Files(root.Profile);
        Assert.IsTrue(files.All(static file => file.StartsWith("cli/sessions/", StringComparison.Ordinal)), string.Join(", ", files));
        Assert.IsTrue(files.Any(file => file.EndsWith(SessionIdOf(run) + ".jsonl", StringComparison.Ordinal)));
        Assert.IsFalse(files.Any(static file => file.Contains("/markers/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Dev_KeepsTheSessionsWithTheDeveloperInstance_AndAnnouncesThemThere()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var run = await root.RunAsync(provider, ["run", Plain, "--dev", "--format", "jsonl", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        var sessionId = SessionIdOf(run);
        var files = CliTestRoot.Files(root.Profile);
        Assert.IsTrue(files.All(static file => file.StartsWith("dev/sessions/", StringComparison.Ordinal)), string.Join(", ", files));
        Assert.IsTrue(files.Any(file => file.EndsWith(sessionId + ".jsonl", StringComparison.Ordinal)));
        Assert.IsNotNull(new SessionMarkerStore(new CatalogOptions { GlobalRoot = root.Profile, StateRoot = Path.Combine(root.Profile, "dev") }).TryRead(sessionId),
            "The developer instance is told of the session.");

        var isolated = await root.RunAsync(provider, ["run", Plain, "--dev", "--isolated", "--format", "jsonl", "hello"]);
        Assert.AreEqual(CliExitCodes.Completed, isolated.ExitCode, isolated.Stderr);
        Assert.IsTrue(CliTestRoot.Files(root.Profile).Any(file => file.StartsWith("dev/cli/sessions/", StringComparison.Ordinal)
            && file.EndsWith(SessionIdOf(isolated) + ".jsonl", StringComparison.Ordinal)));

        var profile = Directory.CreateDirectory(Path.Combine(root.Root, "profile")).FullName;
        var both = await root.RunAsync(provider, ["run", Plain, "--dev", "--profile", profile, "hello"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, both.ExitCode);
    }

    [TestMethod]
    public async Task Profile_MustExist_AndHoldsEverything()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var profile = Path.Combine(root.Root, "profile");

        var missing = await root.RunAsync(provider, ["run", Plain, "--profile", profile, "hello"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, missing.ExitCode);
        StringAssert.Contains(missing.Stderr, "does not exist");

        Directory.CreateDirectory(profile);
        var run = await root.RunAsync(provider, ["run", Plain, "--profile", profile, "--format", "jsonl", "hello"]);
        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        Assert.AreEqual("profile", JsonDocument.Parse(run.Lines[0]).RootElement.GetProperty("params").GetProperty("storage").GetString());
        Assert.IsTrue(CliTestRoot.Files(profile).Any(file => file.StartsWith("sessions/", StringComparison.Ordinal) && file.EndsWith(SessionIdOf(run) + ".jsonl", StringComparison.Ordinal)));
        Assert.AreEqual(0, CliTestRoot.Files(root.Profile).Length, "~/.alta is not touched.");

        root.Variables[CliEnvironment.HomeVariable] = profile;
        var fromVariable = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "hello"]);
        Assert.AreEqual(CliExitCodes.Completed, fromVariable.ExitCode, fromVariable.Stderr);
        Assert.IsTrue(CliTestRoot.Files(profile).Any(file => file.EndsWith(SessionIdOf(fromVariable) + ".jsonl", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Profile_ReadsNoSkillsOrAgentsThatOtherToolsKeepInTheHome()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var skill = Path.Combine(root.Home, ".agents", "skills", "zebra-skill");
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: zebra-skill\ndescription: Paints zebras in stripes.\n---\n\nPaint.\n");
        var agents = Directory.CreateDirectory(Path.Combine(root.Home, ".copilot", "agents")).FullName;
        File.WriteAllText(Path.Combine(agents, "giraffe-agent.agent.md"), "---\nname: giraffe-agent\ndescription: Herds giraffes.\n---\n\nHerd.\n");

        var profile = Directory.CreateDirectory(Path.Combine(root.Root, "profile")).FullName;
        var separate = await root.RunAsync(provider, ["run", Plain, "--profile", profile, "hello"]);
        Assert.AreEqual(CliExitCodes.Completed, separate.ExitCode, separate.Stderr);
        Assert.IsFalse(PromptOfLastRequest(provider).Contains("zebra-skill", StringComparison.Ordinal), "No skill of ~/.agents/skills.");
        Assert.IsFalse(PromptOfLastRequest(provider).Contains("giraffe-agent", StringComparison.Ordinal), "No agent of ~/.copilot/agents.");
        var skills = await root.RunAsync(provider, ["alta", Plain, "--profile", profile, "skill", "list"]);
        Assert.AreEqual(0, skills.ExitCode, skills.Stdout + skills.Stderr);
        Assert.IsFalse(skills.Stdout.Contains("zebra-skill", StringComparison.Ordinal), skills.Stdout);
        var prompts = await root.RunAsync(provider, ["alta", Plain, "--profile", profile, "prompt", "list"]);
        Assert.AreEqual(0, prompts.ExitCode, prompts.Stdout + prompts.Stderr);
        Assert.IsFalse(prompts.Stdout.Contains("giraffe-agent", StringComparison.Ordinal), prompts.Stdout);

        // The other modes read them, as CodeAlta Desktop does, and the alta commands list what the sessions see. The agent
        // prompts are listed with the mode handoff, which a run leaves out by default.
        var visible = await root.RunAsync(provider, ["run", Plain, "--enable", "mode-handoff", "hello"]);
        Assert.AreEqual(CliExitCodes.Completed, visible.ExitCode, visible.Stderr);
        StringAssert.Contains(PromptOfLastRequest(provider), "zebra-skill");
        StringAssert.Contains(PromptOfLastRequest(provider), "giraffe-agent");
        StringAssert.Contains((await root.RunAsync(provider, ["alta", Plain, "skill", "list"])).Stdout, "zebra-skill");
        StringAssert.Contains((await root.RunAsync(provider, ["alta", Plain, "prompt", "list"])).Stdout, "giraffe-agent");
    }

    [TestMethod]
    public async Task Ephemeral_PersistsNothing()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var run = await root.RunAsync(provider, ["run", Plain, "--ephemeral", "--format", "jsonl", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        Assert.AreEqual("visible+ephemeral", JsonDocument.Parse(run.Lines[0]).RootElement.GetProperty("params").GetProperty("storage").GetString());
        Assert.AreEqual(0, CliTestRoot.Files(root.Profile).Length, string.Join(", ", CliTestRoot.Files(root.Profile)));
        Assert.AreEqual(0, CliTestRoot.Files(root.TempRoot).Length, string.Join(", ", CliTestRoot.Files(root.TempRoot)));
    }

    [TestMethod]
    public async Task Config_OverridesApply_AndTheUserFileCanBeIgnored()
    {
        using var root = new CliTestRoot();
        Directory.CreateDirectory(root.Profile);
        File.WriteAllText(Path.Combine(root.Profile, "config.toml"), "[tools]\ndisabled = [\"grep\"]\n");
        var provider = new FakeProvider();

        var fromFile = await root.RunAsync(provider, ["run", Plain, "-c", "tools.disabled=[\"grep\", \"list_dir\"]", "hello"]);
        Assert.AreEqual(CliExitCodes.Completed, fromFile.ExitCode, fromFile.Stderr);
        var tools = ToolsOfLastRequest(provider);
        CollectionAssert.DoesNotContain(tools, "grep");
        CollectionAssert.DoesNotContain(tools, "list_dir", "-c wins over the file.");

        var ignored = await root.RunAsync(provider, ["run", Plain, "--ignore-user-config", "hello"]);
        Assert.AreEqual(CliExitCodes.Completed, ignored.ExitCode, ignored.Stderr);
        CollectionAssert.Contains(ToolsOfLastRequest(provider), "grep", "The user's file is not read.");

        root.Variables[CodeAltaConfigOverlay.InlineContentVariable] = "[tools]\ndisabled = [\"read_file\"]\n";
        var inline = await root.RunAsync(provider, ["run", Plain, "--ignore-user-config", "hello"]);
        Assert.AreEqual(CliExitCodes.Completed, inline.ExitCode, inline.Stderr);
        CollectionAssert.DoesNotContain(ToolsOfLastRequest(provider), "read_file");
        Assert.AreEqual("[tools]\ndisabled = [\"grep\"]\n", File.ReadAllText(Path.Combine(root.Profile, "config.toml")), "The file is never written.");
    }

    [TestMethod]
    public async Task Config_ThatIsInvalidOrUnknownInStrictMode_ExitsWithTwo()
    {
        using var root = new CliTestRoot();
        Directory.CreateDirectory(root.Profile);
        File.WriteAllText(Path.Combine(root.Profile, "config.toml"), "[[[ not toml");
        var provider = new FakeProvider();

        var broken = await root.RunAsync(provider, ["run", Plain, "hello"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, broken.ExitCode, broken.Stderr);
        Assert.AreEqual(CliExitCodes.Completed, (await root.RunAsync(provider, ["run", Plain, "--ignore-user-config", "hello"])).ExitCode);

        var strict = await root.RunAsync(provider, ["run", Plain, "--ignore-user-config", "--strict-config", "-c", "no_such_setting=1", "hello"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, strict.ExitCode);
        StringAssert.Contains(strict.Stderr, "no_such_setting");
        Assert.AreEqual(CliExitCodes.InvalidUsage, (await root.RunAsync(provider, ["run", Plain, "-c", "not an assignment", "hello"])).ExitCode);
    }

    [TestMethod]
    public async Task DisableTools_AndNoSkills_ShapeTheTools()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var skill = Path.Combine(root.Profile, "skills", "zebra-skill");
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: zebra-skill\ndescription: Paints zebras in stripes.\n---\n\nPaint.\n");

        var listed = await root.RunAsync(provider, ["run", Plain, "hello"]);
        Assert.AreEqual(CliExitCodes.Completed, listed.ExitCode, listed.Stderr);
        StringAssert.Contains(PromptOfLastRequest(provider), "zebra-skill", "A skill of the profile is listed by default.");

        var run = await root.RunAsync(provider, ["run", Plain, "--disable-tools", "web,grep", "--no-skills", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        var tools = ToolsOfLastRequest(provider);
        CollectionAssert.DoesNotContain(tools, "webget");
        CollectionAssert.DoesNotContain(tools, "grep");
        CollectionAssert.Contains(tools, "alta");
        Assert.IsFalse(PromptOfLastRequest(provider).Contains("zebra-skill", StringComparison.Ordinal), "--no-skills lists no skill.");
    }

    private static string PromptOfLastRequest(FakeProvider provider)
        => provider.Requests.Last().SystemMessage + "\n" + provider.Requests.Last().DeveloperInstructions;

    internal static string SessionIdOf(CliRun run)
        => JsonDocument.Parse(run.Lines[0]).RootElement.GetProperty("params").GetProperty("sessionId").GetString()!;

    private static string[] ToolsOfLastRequest(FakeProvider provider) => [.. provider.Requests.Last().Tools.Select(static tool => tool.Spec.Name)];
}
